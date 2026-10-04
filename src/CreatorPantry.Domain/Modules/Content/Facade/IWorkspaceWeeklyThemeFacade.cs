using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Content.Business;
using CreatorPantry.Domain.Modules.Content.Managers;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Content.Facade;

/// <summary>
/// The application boundary for the creator's weekly themes: read the week, replace it, delete one theme.
/// </summary>
/// <remarks>
/// Also the seam a consumer that stores a theme key validates through — <see cref="FindAsync"/> — so no caller
/// needs its own notion of which themes a workspace has, and no plugin or job reaches the rows directly.
/// </remarks>
public interface IWorkspaceWeeklyThemeFacade
{
    /// <summary>The resolved workspace's themes, live week first. Every member including a Viewer may read.</summary>
    Task<OperationResult<WeeklyThemeWeekServiceModel>> GetAsync(CancellationToken cancellationToken);

    /// <inheritdoc cref="IWorkspaceWeeklyThemeBusiness.FindAsync"/>
    Task<OperationResult<WeeklyThemeServiceModel>> FindAsync(string key, CancellationToken cancellationToken);

    /// <summary>Replaces the whole week. Editor or above.</summary>
    Task<IdempotentOutcome<WeeklyThemeWeekServiceModel>> ReplaceAsync(
        string userId, ReplaceWeeklyThemesViewModel model, string? idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes one theme outright. Owner only: retirement is reversible and keeps a stored key resolving, and
    /// this does neither, which auth.md puts in the same bracket as any other destructive deletion.
    /// </summary>
    Task<IdempotentOutcome<WeeklyThemeWeekServiceModel>> DeleteAsync(
        string userId, string key, string? idempotencyKey, CancellationToken cancellationToken);
}

internal sealed class WorkspaceWeeklyThemeFacade(
    IValidator<ReplaceWeeklyThemesViewModel> replaceValidator,
    IWorkspaceWeeklyThemeBusiness business,
    IWorkspaceContext workspace,
    IIdempotentCommandExecutor idempotency) : IWorkspaceWeeklyThemeFacade
{
    /// <summary>Stable operation names for the idempotency scope. Changing one orphans in-flight keys.</summary>
    private const string ReplaceOperation = "content.weekly_themes.replace";

    private const string DeleteOperation = "content.weekly_theme.delete";

    private const string CannotSave = "The weekly themes could not be saved.";

    public Task<OperationResult<WeeklyThemeWeekServiceModel>> GetAsync(CancellationToken cancellationToken) =>
        business.GetAsync(cancellationToken);

    public Task<OperationResult<WeeklyThemeServiceModel>> FindAsync(string key, CancellationToken cancellationToken) =>
        business.FindAsync(key, cancellationToken);

    public async Task<IdempotentOutcome<WeeklyThemeWeekServiceModel>> ReplaceAsync(
        string userId, ReplaceWeeklyThemesViewModel model, string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (Forbidden(WorkspaceRole.Editor, "change the weekly themes") is { } forbidden)
        {
            return forbidden;
        }

        var validation = await replaceValidator.ValidateAsync(model, cancellationToken);
        if (!validation.IsValid)
        {
            return Refused(OperationError.Validation(
                ContentErrorCodes.WeeklyThemesInvalid,
                CannotSave,
                validation.Errors.Select(failure => (failure.PropertyName, failure.ErrorMessage))));
        }

        return await idempotency.ExecuteAsync(
            new IdempotentCommand(
                userId, workspace.WorkspaceId, ReplaceOperation, idempotencyKey, model, KeyRequired: false),
            token => business.ReplaceAsync(userId, model, token),
            cancellationToken);
    }

    public async Task<IdempotentOutcome<WeeklyThemeWeekServiceModel>> DeleteAsync(
        string userId, string key, string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (Forbidden(WorkspaceRole.Owner, "delete a weekly theme") is { } forbidden)
        {
            return forbidden;
        }

        return await idempotency.ExecuteAsync(
            new IdempotentCommand(
                userId, workspace.WorkspaceId, DeleteOperation, idempotencyKey, key, KeyRequired: false),
            token => business.DeleteAsync(userId, key, token),
            cancellationToken);
    }

    // The route policy has already refused a lower role; this is the backstop for a worker or plugin reaching the
    // facade directly, which is the whole reason the facade rather than the controller owns the bar.
    private IdempotentOutcome<WeeklyThemeWeekServiceModel>? Forbidden(WorkspaceRole minimum, string action) =>
        workspace.Role < minimum
            ? Refused(new OperationError(
                ContentErrorCodes.WeeklyThemesForbidden,
                $"You do not have permission to {action} in this workspace.",
                new Dictionary<string, string[]>()))
            : null;

    private static IdempotentOutcome<WeeklyThemeWeekServiceModel> Refused(OperationError error) =>
        new(OperationResult<WeeklyThemeWeekServiceModel>.Failure(error), Replayed: false);
}
