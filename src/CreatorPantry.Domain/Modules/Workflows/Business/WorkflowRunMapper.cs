using CreatorPantry.Domain.Modules.Workflows.Data.Entities;
using CreatorPantry.Domain.Modules.Workflows.Managers;

namespace CreatorPantry.Domain.Modules.Workflows.Business;

/// <summary>A run and its rows, as the creator sees it. One builder, so a start and a read (13A.6) cannot drift.</summary>
internal static class WorkflowRunMapper
{
    /// <param name="definition">
    /// The version the run is pinned to — <see cref="WorkflowCatalogue.Find"/>, never the newest — so a run's
    /// goal and step titles are the ones it started with.
    /// </param>
    public static WorkflowRunServiceModel ToServiceModel(
        WorkflowDefinition definition,
        WorkflowRun run,
        IReadOnlyList<WorkflowStepRun> steps,
        IReadOnlyList<WorkflowRunLink> links,
        WorkflowNextActionServiceModel? nextAction,
        IReadOnlyList<WorkflowRunWarningServiceModel>? warnings = null,
        IReadOnlySet<Guid>? missingResourceIds = null)
    {
        var byKey = steps.ToDictionary(step => step.StepKey, StringComparer.Ordinal);

        return new WorkflowRunServiceModel
        {
            Id = run.Id,
            DefinitionKey = run.DefinitionKey,
            DefinitionVersion = run.DefinitionVersion,
            Goal = definition.Goal,
            Title = run.Title,
            Status = run.Status,
            StepCount = run.StepCount,
            CompletedStepCount = run.CompletedStepCount,
            SkippedStepCount = run.SkippedStepCount,
            StartedAt = run.StartedAt,
            UpdatedAt = run.UpdatedAt,
            CompletedAt = run.CompletedAt,
            AbandonedAt = run.AbandonedAt,
            AbandonReason = run.AbandonReason,
            LastRoute = run.LastRoute,
            ReplacedByRunId = run.ReplacedByRunId,
            ConcurrencyToken = WorkflowRunConcurrencyToken.From(run.RowVersion),
            Steps =
            [
                .. definition.Steps.Select(step => new WorkflowStepRunServiceModel
                {
                    Key = step.Key.Value,
                    Title = step.Title,
                    Requirement = step.Requirement,
                    Status = byKey[step.Key.Value].Status,
                    CompletedAt = byKey[step.Key.Value].CompletedAt,
                    SkippedAt = byKey[step.Key.Value].SkippedAt,
                    SkipReason = byKey[step.Key.Value].SkipReason,
                    DraftKind = byKey[step.Key.Value].DraftResourceKind,
                    DraftResourceId = byKey[step.Key.Value].DraftResourceId,
                    DraftSavedAt = byKey[step.Key.Value].DraftSavedAt,
                }),
            ],
            Links =
            [
                .. links.Select(link => new WorkflowRunLinkServiceModel
                {
                    Kind = link.ResourceKind,
                    ResourceId = link.ResourceId,
                    StepKey = link.StepKey,
                }),
            ],
            NextAction = nextAction,

            ResumeRoute = ResumeRoute(run.LastRoute, nextAction, missingResourceIds),
            Warnings = warnings ?? [],

            // Every required step done, on a run still in progress: what "Finish" is offered on (13A.10a).
            ReadyToComplete = run.Status == WorkflowRunStatus.InProgress
                && definition.Steps
                    .Where(step => step.Requirement == WorkflowStepRequirement.Required)
                    .All(step => byKey[step.Key.Value].Status == WorkflowStepRunStatus.Completed),
        };
    }

    /// <summary>
    /// Where "Continue" goes: where the creator last was, unless that address names a record that is gone; else the
    /// first action that exists and has an address. Nowhere once there is nothing to do.
    /// </summary>
    private static string? ResumeRoute(
        string? lastRoute, WorkflowNextActionServiceModel? nextAction, IReadOnlySet<Guid>? missingResourceIds)
    {
        if (nextAction is null)
        {
            return null;
        }

        var lastStillLeadsSomewhere = lastRoute is not null
            && (missingResourceIds is null
                || !missingResourceIds.Any(id => lastRoute.Contains(id.ToString("D"), StringComparison.OrdinalIgnoreCase)));

        return lastStillLeadsSomewhere
            ? lastRoute
            : nextAction.Actions.FirstOrDefault(action => action.Available && action.Route is not null)?.Route;
    }
}
