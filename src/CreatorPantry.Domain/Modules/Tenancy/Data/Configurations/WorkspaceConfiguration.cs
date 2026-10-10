using CreatorPantry.Domain.Modules.Measurement.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Tenancy.Data.Configurations;

internal sealed class WorkspaceConfiguration : IEntityTypeConfiguration<Workspace>
{
    public void Configure(EntityTypeBuilder<Workspace> builder)
    {
        builder.ToTable("Workspaces", table =>
        {
            // Metric (1) or US customary (2) only; see WorkspacePolicy.IsSelectableMeasurementSystem.
            table.HasCheckConstraint(
                "CK_Workspaces_DefaultMeasurementSystem",
                "DefaultMeasurementSystem IN (1, 2)");
        });
        builder.HasKey(workspace => workspace.Id);

        builder.Property(workspace => workspace.Name)
            .IsRequired()
            .HasMaxLength(WorkspacePolicy.NameMaxLength);

        builder.Property(workspace => workspace.Slug)
            .IsRequired()
            .HasMaxLength(WorkspacePolicy.SlugMaxLength);

        builder.Property(workspace => workspace.CreatedAt)
            .IsRequired();

        // The column default is what a workspace created before the setting existed is recorded as. Neutral is
        // the sentinel on purpose: it is never a value a workspace may hold, so "unset" can only mean it.
        builder.Property(workspace => workspace.DefaultMeasurementSystem)
            .IsRequired()
            .HasDefaultValue(WorkspacePolicy.InitialMeasurementSystem)
            .HasSentinel(MeasurementSystem.Neutral);

        // The route resolves a workspace by slug; it must name exactly one.
        builder.HasIndex(workspace => workspace.Slug)
            .IsUnique()
            .HasDatabaseName("UX_Workspaces_Slug");
    }
}
