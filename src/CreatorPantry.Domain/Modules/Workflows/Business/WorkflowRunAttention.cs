using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Workflows.Data;
using CreatorPantry.Domain.Modules.Workflows.Managers;

namespace CreatorPantry.Domain.Modules.Workflows.Business;

/// <summary>
/// What makes a run in progress need a look, in one place: the run list (13A.11) and the dashboard (13A.33a) ask
/// the same question of the same catalogue, so the two can never disagree about which runs are stuck.
/// </summary>
/// <remarks>
/// Every reason is decided by the run's own row against lists built once per request (PERF-001), so neither
/// caller asks anything per run. <see cref="CriteriaFor"/> is what the database matches on and
/// <see cref="ReasonsFor"/> is what a row is then labelled with; both read the same three lists.
/// </remarks>
internal static class WorkflowRunAttention
{
    /// <summary>
    /// The three tests, for a reader of this role: idle past <see cref="WorkflowRunPolicy.IdleAfter"/>; pinned to a
    /// version older than its workflow's newest; or on a step whose actions someone could take and the reader
    /// cannot — the same test the run page uses for <c>next_step_needs_role</c>.
    /// </summary>
    public static WorkflowRunAttentionCriteria CriteriaFor(IWorkflowCatalogue catalogue, WorkspaceRole role, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        var newest = catalogue.All
            .GroupBy(definition => definition.Key.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Max(definition => definition.Version), StringComparer.Ordinal);

        var outdated = catalogue.All
            .Where(definition => definition.Version < newest[definition.Key.Value])
            .Select(definition => WorkflowRunAttentionCriteria.VersionKey(definition.Key.Value, definition.Version))
            .ToList();

        var blocked = catalogue.All
            .SelectMany(definition => definition.Steps
                .Where(step =>
                {
                    var available = Actions(step).Where(capability => capability.IsAvailable).ToList();

                    return available.Count > 0 && !available.Any(capability => role >= capability.MinimumRole);
                })
                .Select(step => WorkflowRunAttentionCriteria.StepKey(definition.Key.Value, definition.Version, step.Key.Value)))
            .ToList();

        return new WorkflowRunAttentionCriteria(now - WorkflowRunPolicy.IdleAfter, outdated, blocked);
    }

    /// <summary>
    /// Why this run needs a look, in the order a reader meets them. Empty for a run that is not in progress: a
    /// completed or abandoned run is nobody's to pick up.
    /// </summary>
    public static IReadOnlyList<string> ReasonsFor(
        WorkflowRunSummaryRecord row,
        DateTimeOffset idleBefore,
        IReadOnlySet<string> outdated,
        IReadOnlySet<string> blocked)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(outdated);
        ArgumentNullException.ThrowIfNull(blocked);

        if (row.Status != WorkflowRunStatus.InProgress)
        {
            return [];
        }

        var reasons = new List<string>();

        if (row.UpdatedAt < idleBefore)
        {
            reasons.Add(WorkflowRunAttentionReasons.Idle);
        }

        if (outdated.Contains(WorkflowRunAttentionCriteria.VersionKey(row.DefinitionKey, row.DefinitionVersion)))
        {
            reasons.Add(WorkflowRunAttentionReasons.NewerVersion);
        }

        if (row.CurrentStepKey is { } current
            && blocked.Contains(WorkflowRunAttentionCriteria.StepKey(row.DefinitionKey, row.DefinitionVersion, current)))
        {
            reasons.Add(WorkflowRunAttentionReasons.NotYoursToDo);
        }

        return reasons;
    }

    /// <summary>A step's main action and its alternatives, as capabilities.</summary>
    public static IEnumerable<WorkflowCapability> Actions(WorkflowStepDefinition step)
    {
        ArgumentNullException.ThrowIfNull(step);

        return new[] { step.Capability.Primary }.Concat(step.Capability.Alternatives).Select(key => WorkflowCapabilities.ByKey[key]);
    }
}
