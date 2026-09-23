using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Tenancy.Data.Configurations;

internal sealed class WorkspaceConfiguration : IEntityTypeConfiguration<Workspace>
{
    public void Configure(EntityTypeBuilder<Workspace> builder)
    {
        builder.ToTable("Workspaces");
        builder.HasKey(workspace => workspace.Id);

        builder.Property(workspace => workspace.Name)
            .IsRequired()
            .HasMaxLength(WorkspacePolicy.NameMaxLength);

        builder.Property(workspace => workspace.Slug)
            .IsRequired()
            .HasMaxLength(WorkspacePolicy.SlugMaxLength);

        builder.Property(workspace => workspace.CreatedAt)
            .IsRequired();

        // The route resolves a workspace by slug; it must name exactly one.
        builder.HasIndex(workspace => workspace.Slug)
            .IsUnique()
            .HasDatabaseName("UX_Workspaces_Slug");
    }
}
