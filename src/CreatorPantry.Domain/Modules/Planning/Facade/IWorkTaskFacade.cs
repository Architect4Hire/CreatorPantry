using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Planning.Business;
using CreatorPantry.Domain.Modules.Planning.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Planning.Facade;

/// <summary>The application boundary for one person's work tasks: quick capture (13A.24).</summary>
public interface IWorkTaskFacade
{
    /// <summary>
    /// Captures a task for the caller in the resolved workspace. Contributor and above; a lower role is
    /// refused with <see cref="PlanningErrorCodes.WorkTaskForbidden"/>.
    /// </summary>
    /// <remarks>
    /// Idempotent under <paramref name="idempotencyKey"/>: a replay returns the first answer and writes
    /// nothing, and the key's record commits with the task or not at all. The key is optional; without one,
    /// two identical requests are two tasks, which for a to-do list is usually what someone meant.
    /// </remarks>
    Task<IdempotentOutcome<WorkTaskServiceModel>> CreateAsync(
        string userId, CreateWorkTaskViewModel model, string? idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// The caller's own day in the resolved workspace (13A.25): what is late, planned for today, under way,
    /// held up, and finished since midnight, with suggestions only when asked for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>No role bar, and none is needed.</strong> A resolved workspace context is the authorization
    /// for the lowest role, and the answer is the caller's own work and nobody else's — the query is keyed
    /// on their membership, so there is no other list to refuse them. A Viewer who was handed work before
    /// being demoted can still see it, which is the right answer.
    /// </para>
    /// <para>
    /// Not cached: it changes with every tick of a task and turns over at midnight in a zone the cache key
    /// would have to carry anyway.
    /// </para>
    /// </remarks>
    Task<OperationResult<TodayWorkTasksServiceModel>> GetTodayAsync(
        bool includeSuggestions, CancellationToken cancellationToken);
}

internal sealed class WorkTaskFacade(
    IValidator<CreateWorkTaskViewModel> createValidator,
    IWorkTaskBusiness business,
    IWorkspaceContext workspace,
    IIdempotentCommandExecutor idempotency) : IWorkTaskFacade
{
    private const string CreateOperation = "planning.work_task.create";

    public async Task<IdempotentOutcome<WorkTaskServiceModel>> CreateAsync(
        string userId, CreateWorkTaskViewModel model, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (Forbidden() is { } forbidden)
        {
            return Refused(forbidden);
        }

        var validation = await createValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return Refused(OperationError.Validation(
                PlanningErrorCodes.WorkTaskInvalid,
                "That task could not be captured as described.",
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        // The submitted fields are the fingerprint: the same key with a different body is a different
        // request. The caller is part of the key's scope, so two people cannot collide on one.
        return await idempotency.ExecuteAsync(
            new IdempotentCommand(userId, workspace.WorkspaceId, CreateOperation, idempotencyKey, model, KeyRequired: false),
            token => business.CreateAsync(model, token),
            cancellationToken);
    }

    // Contributor: a Viewer is a read-only member of the workspace — no work in it, and so no work to track.
    // The route policy has already refused a lower role; this is the backstop for a worker or plugin reaching
    // the facade directly, which is why the facade rather than the controller owns the bar.
    private OperationError? Forbidden() =>
        workspace.Role < WorkspaceRole.Contributor
            ? new OperationError(
                PlanningErrorCodes.WorkTaskForbidden,
                "You do not have permission to keep work in this workspace.",
                new Dictionary<string, string[]>())
            : null;

    private static IdempotentOutcome<WorkTaskServiceModel> Refused(OperationError error) =>
        new(OperationResult<WorkTaskServiceModel>.Failure(error), Replayed: false);

    public Task<OperationResult<TodayWorkTasksServiceModel>> GetTodayAsync(
        bool includeSuggestions, CancellationToken cancellationToken) =>
        business.GetTodayAsync(includeSuggestions, cancellationToken);
}
