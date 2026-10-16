using CreatorPantry.Domain.Managers.Paging;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Workflows.Data;
using CreatorPantry.Domain.Modules.Workflows.Managers;

namespace CreatorPantry.Domain.Modules.Workflows.Business;

public interface IWorkflowRunListBusiness
{
    /// <summary>
    /// One page of the workspace's runs for the reader (13A.11). Members are named by id only; their names are the
    /// workspace module's to give.
    /// </summary>
    Task<OperationResult<CursorPageServiceModel<WorkflowRunSummaryServiceModel>>> ListAsync(
        WorkflowRunListViewModel model, CancellationToken cancellationToken);
}

/// <remarks>
/// <para>
/// <strong>Fixed cost per page (PERF-001).</strong> Two statements here — the page of runs and their links — and one
/// more in the facade to name the people on it, whatever the workspace holds. Everything else a summary says comes from the catalogue in memory: the goal, the step's title, its
/// main action and whether the reader can take it. Nothing is checked per run; a run whose record has gone is the run
/// page's warning (13A.6), not the list's.
/// </para>
/// <para>
/// <strong>One rule for what needs a look</strong>, in <see cref="WorkflowRunAttention"/>: the same lists decide which
/// runs the database returns for the attention view and which reasons each summary carries, so the two cannot
/// disagree — nor can this list and the dashboard's own count of them (13A.33a).
/// </para>
/// </remarks>
internal sealed class WorkflowRunListBusiness(
    IWorkflowRunListDataLayer data,
    IWorkflowCatalogue catalogue,
    IWorkspaceContext workspace,
    IClock clock) : IWorkflowRunListBusiness
{
    public async Task<OperationResult<CursorPageServiceModel<WorkflowRunSummaryServiceModel>>> ListAsync(
        WorkflowRunListViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var attention = AttentionFor(workspace.Role);

        if (!WorkflowRunListQueryFactory.TryCreate(
                model, workspace.WorkspaceId, workspace.MembershipId, workspace.Role, attention, out var criteria))
        {
            return OperationResult<CursorPageServiceModel<WorkflowRunSummaryServiceModel>>.Failure(OperationError.Validation(
                WorkflowErrorCodes.RunListCursorInvalid,
                "This cursor cannot be used with this list. Start again without one.",
                [("cursor", "Start the list again without a cursor.")]));
        }

        var page = await data.ListAsync(criteria!, cancellationToken);
        var linksByRun = page.Links.ToLookup(link => link.WorkflowRunId);
        var outdated = attention.OutdatedVersions.ToHashSet(StringComparer.Ordinal);
        var blocked = attention.BlockedSteps.ToHashSet(StringComparer.Ordinal);

        var items = page.Rows
            .Select(row => ToSummary(row, linksByRun[row.Id], attention.IdleBefore, outdated, blocked))
            .ToList();

        var nextCursor = page.HasMore && page.Rows.Count > 0
            ? ReferenceCursor.Encode(
                WorkflowRunListPosition.FormatSortValue(page.Rows[^1].UpdatedAt), page.Rows[^1].Id.ToString("D"), criteria!.Scope)
            : null;

        return OperationResult<CursorPageServiceModel<WorkflowRunSummaryServiceModel>>.Success(
            new CursorPageServiceModel<WorkflowRunSummaryServiceModel>(items, nextCursor));
    }

    /// <inheritdoc cref="WorkflowRunAttention.CriteriaFor"/>
    private WorkflowRunAttentionCriteria AttentionFor(WorkspaceRole role) =>
        WorkflowRunAttention.CriteriaFor(catalogue, role, clock.UtcNow);

    private WorkflowRunSummaryServiceModel ToSummary(
        WorkflowRunSummaryRecord row,
        IEnumerable<WorkflowRunLinkReferenceRecord> links,
        DateTimeOffset idleBefore,
        IReadOnlySet<string> outdated,
        IReadOnlySet<string> blocked)
    {
        var definition = catalogue.Find(row.DefinitionKey, row.DefinitionVersion);
        var reasons = WorkflowRunAttention.ReasonsFor(row, idleBefore, outdated, blocked);

        // A release that stops shipping a version must not take the creator's own runs with it. Throwing here
        // turned one such row into a 500 for the whole list, for every member of the workspace — nobody could
        // see any of their work because of one run. Everything below comes from the row itself, so the run is
        // still listed and still openable; what only the definition knows is left unsaid rather than guessed.
        if (definition is null)
        {
            return new WorkflowRunSummaryServiceModel
            {
                Id = row.Id,
                Title = row.Title,
                Status = row.Status,
                DefinitionKey = row.DefinitionKey,
                DefinitionVersion = row.DefinitionVersion,
                Goal = "This workflow is no longer available in the version this run started from.",
                Owner = new WorkflowRunMemberServiceModel { MembershipId = row.CreatedByMembershipId },
                Assignee = row.AssignedMembershipId is { } handed ? new WorkflowRunMemberServiceModel { MembershipId = handed } : null,
                StepCount = row.StepCount,
                CompletedStepCount = row.CompletedStepCount,
                SkippedStepCount = row.SkippedStepCount,
                CurrentStep = null,
                LinkedResource = LinkedResource(links),
                NextAction = null,
                StartedAt = row.StartedAt,
                UpdatedAt = row.UpdatedAt,
                CompletedAt = row.CompletedAt,
                AbandonedAt = row.AbandonedAt,
                AttentionReasons = [.. reasons, WorkflowRunAttentionReasons.VersionGone],
            };
        }

        var step = row.Status == WorkflowRunStatus.Completed || row.CurrentStepKey is null
            ? null
            : definition.Steps.FirstOrDefault(candidate => candidate.Key.Value == row.CurrentStepKey);

        WorkflowRunSummaryActionServiceModel? nextAction = null;
        if (row.Status == WorkflowRunStatus.InProgress)
        {
            // Every step done or passed over: the run waits on its last one to be finished, and, as on the run page,
            // there is no next action — finishing is its own command.
            if (step is not null && row.CompletedStepCount + row.SkippedStepCount < row.StepCount)
            {
                nextAction = new WorkflowRunSummaryActionServiceModel
                {
                    StepKey = step.Key.Value,
                    Title = step.Title,
                    CapabilityName = WorkflowCapabilities.ByKey[step.Capability.Primary].Name,
                    YouCanDoIt = WorkflowRunAttention.Actions(step)
                        .Any(capability => capability.IsAvailable && workspace.Role >= capability.MinimumRole),
                };
            }
        }

        return new WorkflowRunSummaryServiceModel
        {
            Id = row.Id,
            Title = row.Title,
            Status = row.Status,
            DefinitionKey = row.DefinitionKey,
            DefinitionVersion = row.DefinitionVersion,
            Goal = definition.Goal,
            Owner = new WorkflowRunMemberServiceModel { MembershipId = row.CreatedByMembershipId },
            Assignee = row.AssignedMembershipId is { } assigned ? new WorkflowRunMemberServiceModel { MembershipId = assigned } : null,
            StepCount = row.StepCount,
            CompletedStepCount = row.CompletedStepCount,
            SkippedStepCount = row.SkippedStepCount,
            CurrentStep = step is null ? null : new WorkflowRunStepReferenceServiceModel { Key = step.Key.Value, Title = step.Title },
            LinkedResource = LinkedResource(links),
            NextAction = nextAction,
            StartedAt = row.StartedAt,
            UpdatedAt = row.UpdatedAt,
            CompletedAt = row.CompletedAt,
            AbandonedAt = row.AbandonedAt,
            AttentionReasons = reasons,
        };
    }

    /// <summary>The record the run started from, or else the last one it made; ordered here, as the run page orders them.</summary>
    private static WorkflowRunResourceReferenceServiceModel? LinkedResource(IEnumerable<WorkflowRunLinkReferenceRecord> links)
    {
        var ordered = links.OrderBy(link => link.LinkedAt).ThenBy(link => link.Id).ToList();
        var chosen = ordered.LastOrDefault(link => link.StepKey is null) ?? ordered.LastOrDefault();

        return chosen is null ? null : new WorkflowRunResourceReferenceServiceModel { Kind = chosen.ResourceKind, Id = chosen.ResourceId };
    }
}
