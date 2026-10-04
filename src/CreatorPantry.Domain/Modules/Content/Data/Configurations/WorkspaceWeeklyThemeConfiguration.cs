using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Content.Data.Configurations;

internal sealed class WorkspaceWeeklyThemeConfiguration : IEntityTypeConfiguration<WorkspaceWeeklyTheme>
{
    public void Configure(EntityTypeBuilder<WorkspaceWeeklyTheme> builder)
    {
        builder.ToTable("WorkspaceWeeklyThemes", table =>
        {
            table.HasCheckConstraint("CK_WorkspaceWeeklyThemes_Key_NotBlank", "trim([Key]) <> ''");
            table.HasCheckConstraint("CK_WorkspaceWeeklyThemes_DisplayName_NotBlank", "trim(DisplayName) <> ''");
            table.HasCheckConstraint("CK_WorkspaceWeeklyThemes_Revision_Positive", "Revision >= 1");

            // DayOfWeek is stored as its integer, and an enum column accepts any integer: a write that went
            // round the write seam could otherwise store a day that does not exist.
            table.HasCheckConstraint("CK_WorkspaceWeeklyThemes_Day_Range", "Day >= 0 AND Day <= 6");
        });

        builder.HasKey(theme => theme.Id);

        // Set by the application, never the store.
        builder.Property(theme => theme.Id).ValueGeneratedNever();

        builder.Property(theme => theme.Day).IsRequired();
        builder.Property(theme => theme.Key).IsRequired().HasMaxLength(WeeklyThemePolicy.KeyMaxLength);
        builder.Property(theme => theme.DisplayName).IsRequired().HasMaxLength(WeeklyThemePolicy.DisplayNameMaxLength);
        builder.Property(theme => theme.Description).HasMaxLength(WeeklyThemePolicy.DescriptionMaxLength);
        builder.Property(theme => theme.Revision).IsRequired();
        builder.Property(theme => theme.CreatedAt).IsRequired();
        builder.Property(theme => theme.UpdatedAt).IsRequired();

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(theme => theme.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // One theme per day — and filtered to the live week, which is the only way that rule and retirement can
        // both hold: an unfiltered index would make a day unusable forever once its first theme retired. Written
        // unquoted so SQL Server and SQLite both accept it. The write seam releases a day before anything else
        // claims it, so this index refuses a genuine second theme rather than a half-applied replace.
        builder.HasIndex(theme => new { theme.WorkspaceId, theme.Day })
            .IsUnique()
            .HasFilter("RetiredAt IS NULL")
            .HasDatabaseName("UX_WorkspaceWeeklyThemes_Workspace_Day");

        // Deliberately *not* filtered: a key is never reused in a workspace, retired or not, so a key stored by
        // some other record resolves to exactly one theme or to nothing at all. That is what makes a stored key
        // trustworthy, and it is also the lookup a consumer validates a key against.
        builder.HasIndex(theme => new { theme.WorkspaceId, theme.Key })
            .IsUnique()
            .HasDatabaseName("UX_WorkspaceWeeklyThemes_Workspace_Key");
    }
}
