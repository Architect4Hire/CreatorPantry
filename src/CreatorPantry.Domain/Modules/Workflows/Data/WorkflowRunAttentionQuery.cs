using CreatorPantry.Domain.Modules.Workflows.Data.Entities;
using CreatorPantry.Domain.Modules.Workflows.Managers;

namespace CreatorPantry.Domain.Modules.Workflows.Data;

/// <summary>
/// The runs that need a look, as a predicate over their own columns. One definition, used by the run list
/// (13A.11) and by the dashboard's count and rows (13A.33a).
/// </summary>
internal static class WorkflowRunAttentionQuery
{
    /// <summary>
    /// In progress, and idle, on an outdated version, or on a step the reader cannot take — each matched on the
    /// run's own columns, the version and step built into the same strings
    /// <see cref="WorkflowRunAttentionCriteria"/> lists.
    /// </summary>
    public static IQueryable<WorkflowRun> NeedingAttention(
        IQueryable<WorkflowRun> runs, WorkflowRunAttentionCriteria attention)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(attention);

        var idleBefore = attention.IdleBefore;
        var outdated = attention.OutdatedVersions;
        var blocked = attention.BlockedSteps;

        return runs.Where(run => run.Status == WorkflowRunStatus.InProgress
            && (run.UpdatedAt < idleBefore
                || outdated.Contains(run.DefinitionKey + WorkflowRunAttentionCriteria.VersionSeparator + run.DefinitionVersion.ToString())
                || blocked.Contains(run.DefinitionKey + WorkflowRunAttentionCriteria.VersionSeparator + run.DefinitionVersion.ToString()
                    + WorkflowRunAttentionCriteria.StepSeparator + run.CurrentStepKey)));
    }
}
