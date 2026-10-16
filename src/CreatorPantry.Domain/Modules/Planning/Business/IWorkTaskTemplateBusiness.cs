using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Planning.Data;
using CreatorPantry.Domain.Modules.Planning.Data.Entities;
using CreatorPantry.Domain.Modules.Planning.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Facade;

namespace CreatorPantry.Domain.Modules.Planning.Business;

/// <summary>The domain rules for making a routine's occurrences (13A.30).</summary>
public interface IWorkTaskTemplateBusiness
{
    /// <summary>
    /// Makes every occurrence of one routine from today through the lookahead, in its own zone, for a pass that
    /// holds its lease, and lets the lease go.
    /// </summary>
    Task<WorkTaskTemplateRun> MaterializeAsync(Guid templateId, Guid leaseToken, CancellationToken cancellationToken);
}

internal sealed class WorkTaskTemplateBusiness(
    IWorkTaskTemplateDataLayer dataLayer,
    IWorkspaceFacade workspaces,
    ITimeZoneConverter zones,
    IClock clock) : IWorkTaskTemplateBusiness
{
    public async Task<WorkTaskTemplateRun> MaterializeAsync(Guid templateId, Guid leaseToken, CancellationToken cancellationToken)
    {
        var template = await dataLayer.FindForUpdateAsync(templateId, cancellationToken);

        // Gone, another workspace's, or no longer held by this pass — taken by another, or lapsed: not this pass's to
        // touch. The row version would refuse a write anyway; this stops before trying.
        if (template is null || template.LeasedBy != leaseToken || template.LeaseExpiresAt <= clock.UtcNow)
        {
            return new WorkTaskTemplateRun(WorkTaskTemplateOutcome.Skipped, 0);
        }

        if (template.Status != WorkTaskTemplateStatus.Active)
        {
            return await LetGoAsync(template, WorkTaskTemplateOutcome.Skipped, 0, cancellationToken);
        }

        // Its own zone, not the workspace's now: a later edit to brand settings does not move a routine's days.
        if (!zones.IsValidZone(template.TimeZoneId))
        {
            return await StopAsync(template, WorkTaskTemplateAttention.TimeZoneInvalid, cancellationToken);
        }

        // Each occurrence is its owner's, so its owner must still be able to keep work here.
        var roles = await workspaces.FindActiveMemberRolesAsync([template.OwnerMembershipId], cancellationToken);
        if (!roles.TryGetValue(template.OwnerMembershipId, out var role) || role < WorkspaceRole.Contributor)
        {
            return await StopAsync(template, WorkTaskTemplateAttention.OwnerUnavailable, cancellationToken);
        }

        var today = zones.LocalDate(clock.UtcNow, template.TimeZoneId);
        var horizon = WorkTaskRecurrence.HorizonFrom(today);

        // From the stored next date, or from today when that is behind it: never made late — a pass that was down
        // past the lookahead, or a routine never yet seen, starts again at today, and a date missed is missed.
        // The first match is taken against the routine as it is now, so a schedule changed since the next date was
        // worked out is honoured rather than making one more day of the old pattern.
        var from = template.NextOccurrenceDate is { } due && due > today ? due : today;
        var next = WorkTaskRecurrence.FirstOnOrAfter(from, template.StartsOn, template.EndsOn, template.Frequency, template.Weekdays);

        var made = 0;

        while (next is { } date && date <= horizon)
        {
            template.NextOccurrenceDate = WorkTaskRecurrence.FirstOnOrAfter(
                date.AddDays(1), template.StartsOn, template.EndsOn, template.Frequency, template.Weekdays);
            template.UpdatedAt = clock.UtcNow;

            switch (await dataLayer.SaveOccurrenceAsync(template, Occurrence(template, date), cancellationToken))
            {
                case WorkTaskOccurrenceWrite.Made:
                    made++;
                    break;
                case WorkTaskOccurrenceWrite.AlreadyMade:
                    break;
                default:
                    // Changed under this pass. Nothing of this date landed; the lease lapses and the next pass
                    // takes it from where the store says it is.
                    return new WorkTaskTemplateRun(WorkTaskTemplateOutcome.Stale, made);
            }

            next = template.NextOccurrenceDate;
        }

        template.NextOccurrenceDate = next;

        if (next is null)
        {
            template.Status = WorkTaskTemplateStatus.Ended;
            return await LetGoAsync(template, WorkTaskTemplateOutcome.Ended, made, cancellationToken);
        }

        return await LetGoAsync(template, WorkTaskTemplateOutcome.Materialized, made, cancellationToken);
    }

    /// <summary>
    /// One occurrence: its owner's task, not started, on its date and in that date's week, with the routine's
    /// default fields and its pointer back to the routine and the occurrence.
    /// </summary>
    private WorkTask Occurrence(WorkTaskTemplate template, DateOnly date)
    {
        var now = clock.UtcNow;

        return new WorkTask
        {
            Id = Guid.NewGuid(),
            OwnerMembershipId = template.OwnerMembershipId,
            AssigneeMembershipId = template.OwnerMembershipId,
            Title = template.Title,
            Description = template.Description,
            Status = WorkTaskStatus.Backlog,
            Priority = template.Priority,
            EstimateMinutes = template.EstimateMinutes,
            ScheduledDate = date,
            PlanningWeekStart = WorkTaskWeek.StartOf(date),
            WorkTaskTemplateId = template.Id,
            RecurrenceDate = date,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private async Task<WorkTaskTemplateRun> StopAsync(
        WorkTaskTemplate template, WorkTaskTemplateAttention reason, CancellationToken cancellationToken)
    {
        template.Status = WorkTaskTemplateStatus.NeedsAttention;
        template.AttentionReason = reason;
        template.UpdatedAt = clock.UtcNow;

        return await LetGoAsync(template, WorkTaskTemplateOutcome.NeedsAttention, 0, cancellationToken);
    }

    private async Task<WorkTaskTemplateRun> LetGoAsync(
        WorkTaskTemplate template, WorkTaskTemplateOutcome outcome, int made, CancellationToken cancellationToken)
    {
        template.LeasedBy = null;
        template.LeaseExpiresAt = null;

        return await dataLayer.SaveTemplateAsync(template, cancellationToken)
            ? new WorkTaskTemplateRun(outcome, made)
            : new WorkTaskTemplateRun(WorkTaskTemplateOutcome.Stale, made);
    }
}
