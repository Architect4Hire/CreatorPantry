using CreatorPantry.Domain.Modules.Planning.Data.Entities;
using CreatorPantry.Domain.Modules.Planning.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CreatorPantry.Domain.Modules.Planning.Data.Configurations;

internal sealed class WorkTaskTemplateConfiguration : IEntityTypeConfiguration<WorkTaskTemplate>
{
    public void Configure(EntityTypeBuilder<WorkTaskTemplate> builder)
    {
        const int weekly = (int)WorkTaskRecurrenceFrequency.Weekly;
        const int ended = (int)WorkTaskTemplateStatus.Ended;
        const int needsAttention = (int)WorkTaskTemplateStatus.NeedsAttention;

        builder.ToTable("WorkTaskTemplates", table =>
        {
            table.HasCheckConstraint("CK_WorkTaskTemplates_Title_NotBlank", "trim(Title) <> ''");
            table.HasCheckConstraint("CK_WorkTaskTemplates_Description_NotBlank", "Description IS NULL OR trim(Description) <> ''");
            table.HasCheckConstraint("CK_WorkTaskTemplates_TimeZone_NotBlank", "trim(TimeZoneId) <> ''");

            table.HasCheckConstraint(
                "CK_WorkTaskTemplates_Priority_Range",
                $"Priority >= {(int)WorkTaskPriority.Low} AND Priority <= {(int)WorkTaskPriority.High}");
            table.HasCheckConstraint(
                "CK_WorkTaskTemplates_Estimate_Range",
                $"EstimateMinutes IS NULL OR (EstimateMinutes >= 1 AND EstimateMinutes <= {WorkTaskPolicy.EstimateMaxMinutes})");
            table.HasCheckConstraint(
                "CK_WorkTaskTemplates_Frequency_Range",
                $"Frequency >= {(int)WorkTaskRecurrenceFrequency.Daily} AND Frequency <= {weekly}");
            table.HasCheckConstraint(
                "CK_WorkTaskTemplates_Status_Range",
                $"Status >= {(int)WorkTaskTemplateStatus.Active} AND Status <= {needsAttention}");

            // A weekly routine names at least one weekday and only real ones; a daily one names none.
            table.HasCheckConstraint(
                "CK_WorkTaskTemplates_Weekdays_Frequency",
                $"(Frequency = {weekly} AND Weekdays >= 1 AND Weekdays <= {(int)WorkTaskWeekdays.All}) OR "
                    + $"(Frequency <> {weekly} AND Weekdays = 0)");

            // Comparisons, which both engines write the same way: an end not before the start, and a next date
            // inside the two.
            table.HasCheckConstraint("CK_WorkTaskTemplates_Ends_AfterStart", "EndsOn IS NULL OR EndsOn >= StartsOn");
            table.HasCheckConstraint(
                "CK_WorkTaskTemplates_Next_InRange",
                "NextOccurrenceDate IS NULL OR (NextOccurrenceDate >= StartsOn AND (EndsOn IS NULL OR NextOccurrenceDate <= EndsOn))");

            // An ended routine has nothing next; a reason travels with "needs attention" and only with it.
            table.HasCheckConstraint("CK_WorkTaskTemplates_Ended_NoNext", $"Status <> {ended} OR NextOccurrenceDate IS NULL");
            table.HasCheckConstraint(
                "CK_WorkTaskTemplates_Attention_Reason",
                $"(Status = {needsAttention} AND AttentionReason IS NOT NULL) OR (Status <> {needsAttention} AND AttentionReason IS NULL)");
            table.HasCheckConstraint(
                "CK_WorkTaskTemplates_Attention_Range",
                $"AttentionReason IS NULL OR (AttentionReason >= {(int)WorkTaskTemplateAttention.OwnerUnavailable} "
                    + $"AND AttentionReason <= {(int)WorkTaskTemplateAttention.TimeZoneInvalid})");

            // A lease is whole or absent.
            table.HasCheckConstraint(
                "CK_WorkTaskTemplates_Lease_Whole",
                "(LeasedBy IS NULL AND LeaseExpiresAt IS NULL) OR (LeasedBy IS NOT NULL AND LeaseExpiresAt IS NOT NULL)");
        });

        builder.HasKey(template => template.Id);
        builder.Property(template => template.Id).ValueGeneratedNever();

        // Target of the occurrences' composite key, so a task cannot claim another workspace's template.
        builder.HasAlternateKey(template => new { template.WorkspaceId, template.Id })
            .HasName("AK_WorkTaskTemplates_Workspace_Id");

        builder.Property(template => template.OwnerMembershipId).IsRequired();
        builder.Property(template => template.Title).IsRequired().HasMaxLength(WorkTaskPolicy.TitleMaxLength);
        builder.Property(template => template.Description).HasMaxLength(WorkTaskPolicy.DescriptionMaxLength);
        builder.Property(template => template.Priority).IsRequired();
        builder.Property(template => template.Frequency).IsRequired();
        builder.Property(template => template.Weekdays).IsRequired();
        builder.Property(template => template.StartsOn).IsRequired();
        builder.Property(template => template.TimeZoneId).IsRequired().HasMaxLength(WorkTaskPolicy.TimeZoneIdMaxLength);
        builder.Property(template => template.Status).IsRequired();
        builder.Property(template => template.CreatedAt).IsRequired();
        builder.Property(template => template.UpdatedAt).IsRequired();
        builder.Property(template => template.RowVersion).IsRowVersion();

        builder.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(template => template.WorkspaceId)
            .OnDelete(DeleteBehavior.Cascade);

        // The worker's scan for due routines across workspaces: active, by next date.
        builder.HasIndex(template => new { template.Status, template.NextOccurrenceDate })
            .HasDatabaseName("IX_WorkTaskTemplates_Status_Next");

        // One person's routines.
        builder.HasIndex(template => new { template.WorkspaceId, template.OwnerMembershipId, template.Status })
            .HasDatabaseName("IX_WorkTaskTemplates_Workspace_Owner_Status");
    }
}
