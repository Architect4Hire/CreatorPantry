using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Workflows.Data;
using CreatorPantry.Domain.Modules.Workflows.Managers;

namespace CreatorPantry.Domain.Modules.Workflows.Business;

/// <summary>The workflow module's part of the workspace dashboard (13A.33a, FLOW-004).</summary>
public interface IWorkflowDashboardBusiness
{
    /// <summary>
    /// The caller's own runs in progress and the workspace's runs that need a look, each counted in full and the
    /// first few named.
    /// </summary>
    Task<WorkflowDashboardSummaryServiceModel> GetSummaryAsync(CancellationToken cancellationToken);
}

/// <remarks>
/// <para>
/// <strong>Read-only, and bounded.</strong> Two counts and two lists of at most
/// <see cref="WorkflowDashboardPolicy.ListLimit"/>; the goal, the step's title and whether the reader may take it
/// come from the catalogue in memory, as they do in the run list, so nothing is read per run (PERF-001).
/// </para>
/// <para>
/// <strong>One rule for what needs a look</strong>, <see cref="WorkflowRunAttention"/>'s: the dashboard's count,
/// its rows and the hub's attention list are the same question asked of the same catalogue, for the same reader.
/// </para>
/// <para>
/// A run pinned to a version the catalogue no longer has is left out of the lists rather than throwing: the run
/// page is where a pinned version that has gone is explained (13A.6), and one such run must not cost a creator
/// their whole dashboard. It is still in the counts, which are the database's.
/// </para>
/// </remarks>
internal sealed class WorkflowDashboardBusiness(
    IWorkflowDashboardDataLayer data,
    IWorkflowCatalogue catalogue,
    IWorkspaceContext workspace,
    IClock clock) : IWorkflowDashboardBusiness
{
    public async Task<WorkflowDashboardSummaryServiceModel> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var attention = WorkflowRunAttention.CriteriaFor(catalogue, workspace.Role, clock.UtcNow);
        var read = await data.ReadAsync(
            workspace.MembershipId, attention, WorkflowDashboardPolicy.ListLimit, cancellationToken);

        var outdated = attention.OutdatedVersions.ToHashSet(StringComparer.Ordinal);
        var blocked = attention.BlockedSteps.ToHashSet(StringComparer.Ordinal);

        return new WorkflowDashboardSummaryServiceModel(
            read.InProgressCount,
            [.. read.Mine.Select(row => ToRun(row, attention.IdleBefore, outdated, blocked)).OfType<WorkflowDashboardRunServiceModel>()],
            read.NeedsAttentionCount,
            [.. read.NeedsAttention.Select(row => ToRun(row, attention.IdleBefore, outdated, blocked)).OfType<WorkflowDashboardRunServiceModel>()]);
    }

    /// <summary>One row as the dashboard names it, or null when its pinned version is no longer shipped.</summary>
    private WorkflowDashboardRunServiceModel? ToRun(
        WorkflowRunSummaryRecord row,
        DateTimeOffset idleBefore,
        IReadOnlySet<string> outdated,
        IReadOnlySet<string> blocked)
    {
        var definition = catalogue.Find(row.DefinitionKey, row.DefinitionVersion);
        if (definition is null)
        {
            return null;
        }

        // The step it is on, and only while there is one left to do: a run waiting to be finished has no next
        // step, as the run list and the run page agree (13A.11, 13A.6).
        var step = row.CompletedStepCount + row.SkippedStepCount < row.StepCount && row.CurrentStepKey is { } key
            ? definition.Steps.FirstOrDefault(candidate => candidate.Key.Value == key)
            : null;

        return new WorkflowDashboardRunServiceModel(
            row.Id,
            row.Title,
            row.DefinitionKey,
            definition.Goal,
            step?.Key.Value,
            step?.Title,
            CanAct(row, step),
            row.UpdatedAt,
            WorkflowRunAttention.ReasonsFor(row, idleBefore, outdated, blocked));
    }

    /// <summary>
    /// Whether the reader can take this run forward themselves. On a step, that is whether the step offers an
    /// action their role may take. With no step left, the run is waiting to be finished, which is not the same
    /// as waiting for somebody else: finishing it is the owner's, the person it is for, or an Editor's and
    /// above — the rule that governs every other change to a run. Reading "no step" as "not yours" showed a
    /// "Needs someone else" warning beside "Next: Finish it" on a run the reader could finish.
    /// </summary>
    private bool CanAct(WorkflowRunSummaryRecord row, WorkflowStepDefinition? step) =>
        step is not null
            ? WorkflowRunAttention.Actions(step).Any(capability => capability.IsAvailable && workspace.Role >= capability.MinimumRole)
            : workspace.Role >= WorkspaceRole.Editor
                || row.CreatedByMembershipId == workspace.MembershipId
                || row.AssignedMembershipId == workspace.MembershipId;
}
