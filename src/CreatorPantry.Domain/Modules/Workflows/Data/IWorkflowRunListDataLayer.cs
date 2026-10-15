using CreatorPantry.Domain.Modules.Workflows.Managers;

namespace CreatorPantry.Domain.Modules.Workflows.Data;

/// <summary>The run list's reads (13A.11). Read-only: it owns no transaction and writes nothing.</summary>
public interface IWorkflowRunListDataLayer
{
    /// <summary>One page of runs, and every link of the runs on it — two statements, however long the list.</summary>
    Task<WorkflowRunListPage> ListAsync(WorkflowRunListCriteria criteria, CancellationToken cancellationToken);
}

public sealed record WorkflowRunListPage(
    IReadOnlyList<WorkflowRunSummaryRecord> Rows,
    bool HasMore,
    IReadOnlyList<WorkflowRunLinkReferenceRecord> Links);

internal sealed class WorkflowRunListDataLayer(IWorkflowRunListRepository runs) : IWorkflowRunListDataLayer
{
    public async Task<WorkflowRunListPage> ListAsync(WorkflowRunListCriteria criteria, CancellationToken cancellationToken)
    {
        var (rows, hasMore) = await runs.ListAsync(criteria, cancellationToken);
        var links = await runs.ListLinksAsync([.. rows.Select(row => row.Id)], cancellationToken);

        return new WorkflowRunListPage(rows, hasMore, links);
    }
}
