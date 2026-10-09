using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Content.Data.Configurations;

internal sealed class CreativeContextConfiguration : IEntityTypeConfiguration<CreativeContext>
{
    public void Configure(EntityTypeBuilder<CreativeContext> builder)
    {
        builder.ToTable("CreativeContexts", table =>
        {
            // Optional means absent, not empty: a blank title would list as a nameless row and a blank brief
            // would reach the prompt step as a description of nothing.
            table.HasCheckConstraint(
                "CK_CreativeContexts_WorkingTitle_NotBlank",
                "WorkingTitle IS NULL OR trim(WorkingTitle) <> ''");
            table.HasCheckConstraint(
                "CK_CreativeContexts_PictureBrief_NotBlank",
                "PictureBrief IS NULL OR trim(PictureBrief) <> ''");
            table.HasCheckConstraint(
                "CK_CreativeContexts_WorkingBrief_NotBlank",
                "WorkingBrief IS NULL OR trim(WorkingBrief) <> ''");

            // Stored as its integer, and an enum column accepts any integer.
            table.HasCheckConstraint(
                "CK_CreativeContexts_BriefSource_Range",
                "BriefSource IS NULL OR (BriefSource >= 1 AND BriefSource <= 3)");
            table.HasCheckConstraint(
                "CK_CreativeContexts_WeeklyThemeKey_NotBlank",
                "WeeklyThemeKey IS NULL OR trim(WeeklyThemeKey) <> ''");

            // DayOfWeek is stored as its integer, and an enum column accepts any integer.
            table.HasCheckConstraint("CK_CreativeContexts_Day_Range", "Day IS NULL OR (Day >= 0 AND Day <= 6)");
        });

        builder.HasKey(context => context.Id);

        // Set by the application, never the store.
        builder.Property(context => context.Id).ValueGeneratedNever();

        builder.Property(context => context.WorkingTitle).HasMaxLength(CreativeContextPolicy.WorkingTitleMaxLength);
        builder.Property(context => context.PictureBrief).HasMaxLength(CreativeContextPolicy.PictureBriefMaxLength);
        builder.Property(context => context.WorkingBrief).HasMaxLength(CreativeContextPolicy.WorkingBriefMaxLength);
        builder.Property(context => context.WeeklyThemeKey).HasMaxLength(WeeklyThemePolicy.KeyMaxLength);
        builder.Property(context => context.CreatedAt).IsRequired();
        builder.Property(context => context.UpdatedAt).IsRequired();
        builder.Property(context => context.CreatedByMembershipId).IsRequired();
        builder.Property(context => context.RowVersion).IsRowVersion();

        // The erasure path, and the only cascade into this table.
        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(context => context.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // The recent list: live contexts, most recently touched first, with the id as the keyset tie-break.
        // Filtered because an archived context is never listed there. Written unquoted so SQL Server and
        // SQLite both accept it.
        builder.HasIndex(context => new { context.WorkspaceId, context.UpdatedAt, context.Id })
            .IsDescending(false, true, true)
            .HasFilter("ArchivedAt IS NULL")
            .HasDatabaseName("IX_CreativeContexts_Workspace_Updated");

        // "What has this workspace made for this themed day". Not filtered on ArchivedAt: archiving a context
        // does not unmake what came of it. Filtered on the key because most contexts carry none.
        builder.HasIndex(context => new { context.WorkspaceId, context.WeeklyThemeKey, context.UpdatedAt })
            .IsDescending(false, false, true)
            .HasFilter("WeeklyThemeKey IS NOT NULL")
            .HasDatabaseName("IX_CreativeContexts_Workspace_Theme_Updated");
    }
}
