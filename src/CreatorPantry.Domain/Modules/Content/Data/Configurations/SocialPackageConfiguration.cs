using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Content.Data.Configurations;

internal sealed class SocialPackageConfiguration : IEntityTypeConfiguration<SocialPackage>
{
    public void Configure(EntityTypeBuilder<SocialPackage> builder)
    {
        builder.ToTable("SocialPackages");

        builder.HasKey(package => package.Id);

        // Set by the application, never the store.
        builder.Property(package => package.Id).ValueGeneratedNever();

        // Target of the channel's composite key.
        builder.HasAlternateKey(package => new { package.WorkspaceId, package.Id })
            .HasName("AK_SocialPackages_Workspace_Id");

        builder.Property(package => package.CreatedByMembershipId).IsRequired();
        builder.Property(package => package.CreatedAt).IsRequired();
        builder.Property(package => package.UpdatedAt).IsRequired();

        // The erasure path, and the only cascade into this table.
        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(package => package.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Workspace-paired, so a package for another workspace's context is unrepresentable. Restricted: a
        // context is archived rather than removed, and a second cascade path is what SQL Server refuses.
        builder.HasOne<CreativeContext>()
            .WithMany()
            .HasForeignKey(package => new { package.WorkspaceId, package.CreativeContextId })
            .HasPrincipalKey(context => new { context.WorkspaceId, context.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // One package per piece of work; more channels are more slots, not another package.
        builder.HasIndex(package => new { package.WorkspaceId, package.CreativeContextId })
            .IsUnique()
            .HasDatabaseName("UX_SocialPackages_Workspace_Context");
    }
}
