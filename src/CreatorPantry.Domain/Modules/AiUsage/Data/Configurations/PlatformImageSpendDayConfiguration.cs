using CreatorPantry.Domain.Modules.AiUsage.Data.Entities;
using CreatorPantry.Domain.Modules.AiUsage.Managers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.AiUsage.Data.Configurations;

internal sealed class PlatformImageSpendDayConfiguration : IEntityTypeConfiguration<PlatformImageSpendDay>
{
    public void Configure(EntityTypeBuilder<PlatformImageSpendDay> builder)
    {
        builder.ToTable("PlatformImageSpendDays", table =>
        {
            // A negative total would hand the ceiling more room than it has. The give-back of a never-queued
            // request is the only subtraction, and it returns exactly what that request added.
            table.HasCheckConstraint(
                "CK_PlatformImageSpendDays_Admitted_NotNegative",
                "AdmittedMicros >= 0");

            // A lift names who lifted it, and nothing names a lifter that did not lift.
            table.HasCheckConstraint(
                "CK_PlatformImageSpendDays_Lift_Complete",
                "(LiftedAt IS NULL AND LiftedBy IS NULL) OR (LiftedAt IS NOT NULL AND LiftedBy IS NOT NULL)");
        });

        builder.HasKey(day => day.Day);

        // The day is the identity, never generated: two instances creating it at once collide on the key and
        // the loser reads the winner's row.
        builder.Property(day => day.Day).ValueGeneratedNever();

        builder.Property(day => day.AdmittedMicros).IsRequired();

        builder.Property(day => day.LiftedBy).HasMaxLength(AiUsagePolicy.ProviderIdentifierMaxLength);
    }
}
