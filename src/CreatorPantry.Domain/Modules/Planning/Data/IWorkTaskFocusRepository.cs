using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Planning.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CreatorPantry.Domain.Modules.Planning.Data;

/// <summary>EF Core access to one person's top three (13A.32). Every query is already scoped to the resolved workspace.</summary>
public interface IWorkTaskFocusRepository
{
    /// <summary>The tasks chosen for one person's day, by id, in the order they were put in.</summary>
    Task<IReadOnlyList<Guid>> ChosenAsync(Guid assigneeMembershipId, DateOnly focusDate, CancellationToken cancellationToken);

    /// <summary>The same rows, tracked, so a new choice can replace them.</summary>
    Task<List<WorkTaskFocus>> RowsForUpdateAsync(Guid assigneeMembershipId, DateOnly focusDate, CancellationToken cancellationToken);

    /// <summary>
    /// The named tasks as they stand, untracked — whichever of them exist in the resolved workspace. Read to judge
    /// whether each is on the caller's day before any is chosen.
    /// </summary>
    Task<IReadOnlyList<WorkTask>> TasksAsync(IReadOnlyCollection<Guid> taskIds, CancellationToken cancellationToken);

    /// <summary>Stages a place. <c>WorkspaceId</c> is stamped by the ownership interceptor, never here.</summary>
    void Add(WorkTaskFocus focus);

    void RemoveRange(IEnumerable<WorkTaskFocus> rows);
}

internal sealed class WorkTaskFocusRepository(CreatorPantryDbContext context) : IWorkTaskFocusRepository
{
    public async Task<IReadOnlyList<Guid>> ChosenAsync(
        Guid assigneeMembershipId, DateOnly focusDate, CancellationToken cancellationToken) =>
        await Day(assigneeMembershipId, focusDate)
            .AsNoTracking()
            .OrderBy(focus => focus.Position)
            .Select(focus => focus.WorkTaskId)
            .ToListAsync(cancellationToken);

    public Task<List<WorkTaskFocus>> RowsForUpdateAsync(
        Guid assigneeMembershipId, DateOnly focusDate, CancellationToken cancellationToken) =>
        Day(assigneeMembershipId, focusDate).OrderBy(focus => focus.Position).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<WorkTask>> TasksAsync(IReadOnlyCollection<Guid> taskIds, CancellationToken cancellationToken) =>
        taskIds.Count == 0
            ? []
            : await context.WorkTasks.AsNoTracking().Where(task => taskIds.Contains(task.Id)).ToListAsync(cancellationToken);

    public void Add(WorkTaskFocus focus) => context.WorkTaskFocuses.Add(focus);

    public void RemoveRange(IEnumerable<WorkTaskFocus> rows) => context.WorkTaskFocuses.RemoveRange(rows);

    private IQueryable<WorkTaskFocus> Day(Guid assigneeMembershipId, DateOnly focusDate) =>
        context.WorkTaskFocuses.Where(focus =>
            focus.AssigneeMembershipId == assigneeMembershipId && focus.FocusDate == focusDate);
}
